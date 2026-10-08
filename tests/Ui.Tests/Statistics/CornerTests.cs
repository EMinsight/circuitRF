using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Tests.Optimization;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>brief-yield-6's benches: a 1 V divider whose R1 has a temperature coefficient, so a corner's
/// <c>temp</c> moves Vout. At 125 °C R1 is 1000·(1 + 0.004·98.15) Ω and Vout ≈ 0.418 V, below the goal's 0.42.</summary>
internal static class CornerCircuits
{
    public const string Divider = """
        Vdc:V1 in  0   Vdc=1
        R:R1   in  out R=1000 Ohm TC1=0.004
        R:R2   out 0   R=1000 Ohm
        analysis DC1 type=dc
        goal Vout = DC1.V("out") analysis=DC1 ge 0.42 use=yield
        """;

    public const string Corners = """
        corner cold temp=-40
        corner hot temp=125
        corner hiR2 temp=85 R2.R=1100 Ohm
        """;

    /// <summary>The goal's margin of <paramref name="cnl"/> evaluated as it stands — what Simulate gives with those
    /// values typed.</summary>
    public static double TypedMargin(string cnl)
    {
        var run = OptimizationRun.ForEvaluation(PreparedCircuit.FromText(cnl, null, null), goals: GoalUse.Yield);
        Assert.Null(run.Refusal);
        var p = Assert.Single(run.EvaluateValues([new Dictionary<string, string>()]));
        Assert.Equal(PointStatus.Evaluated, p.Status);
        return Assert.Single(p.Goals).Margin;
    }
}

/// <summary>R-ya6-1, R-ya6-2: a corner evaluates as its values typed; a corner's kit axis replaces only that axis.</summary>
public sealed class CornerRunTests
{
    [Fact]
    public void EachCornersMargin_EqualsASimulateWithItsValuesTyped()
    {
        var r = CornerRun.Create(PreparedCircuit.FromText(CornerCircuits.Divider + "\n" + CornerCircuits.Corners, null, null)).Run();

        Assert.Equal(StatisticalOutcome.BelowTarget, r.Outcome);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal(["nominal", "cold", "hot", "hiR2"], r.Corners.Select(c => c.Name));
        var typed = new[]
        {
            CornerCircuits.Divider,
            CornerCircuits.Divider + "\ntemp = -40\n",
            CornerCircuits.Divider + "\ntemp = 125\n",
            CornerCircuits.Divider.Replace("out 0   R=1000 Ohm", "out 0   R=1100 Ohm") + "\ntemp = 85\n",
        };
        for (int i = 0; i < typed.Length; i++)
            Assert.Equal(CornerCircuits.TypedMargin(typed[i]), r.Corners[i].Goals.Single().Margin);

        Assert.Equal([true, true, false, true], r.Corners.Select(c => c.Pass));
        var worst = Assert.Single(r.Worst);
        Assert.Equal(("Vout", "hot"), (worst.Goal, worst.Corner));

        // One DataSet, the corner names on its outer axis.
        var margin = r.Data!["corners.goal:Vout:margin"];
        Assert.Equal(["nominal", "cold", "hot", "hiR2"], margin.Axes[0].Labels!);
        Assert.Equal("corner", r.Data["DC1.V"].Axes[0].Name);
    }

    [Fact]
    public void ACornerSettingOneKitAxis_LeavesTheSchematicsOtherAxisInForce()
    {
        string kit = FixturePaths.Require("testdata/corner-kit");
        var axes = WorkspaceCorners.From(Path.GetDirectoryName(kit)!,
        [
            new CwsPdkRef
            {
                Path = kit, Provider = "SynthKit",
                Corners =
                [
                    new CwsCornerAxis { AxisId = "models/r1Corners.lib", DisplayName = "r1Corners", Options = ["r1_typ", "r1_hi"] },
                    new CwsCornerAxis { AxisId = "models/r2Corners.lib", DisplayName = "r2Corners", Options = ["r2_typ", "r2_hi"] },
                ],
            },
        ]);
        string r1 = axes.Single(a => a.DisplayName == "r1Corners").Key, r2 = axes.Single(a => a.DisplayName == "r2Corners").Key;

        // The schematic selects r2_hi; its corner selects r1_hi and says nothing of r2.
        var model = new SchematicEditModel();
        model.CornerSelections[r2] = "r2_hi";
        model.Tuning = new TuningSetup { Corners = [new CornerDefinition { Name = "r1hi", AxisSelections = new() { [r1] = "r1_hi" } }] };
        var problems = new List<string>();
        var bound = WorkspaceCorners.Bind(axes, model.CornerSelections, problems);
        var extracted = NetExtractor.Extract(model, "tb", cornerVariables: bound.Variables,
            cornerBinder: (s, p) => WorkspaceCorners.BindingsFor(axes, s, p), cornerSections: bound.Sections);
        Assert.Empty(problems);

        string cnl = CnlWriter.Write(extracted.TestBench) + """

            Vdc:V1 in  0   Vdc=1
            R:R1   in  out R=r1val
            R:R2   out 0   R=r2val
            analysis DC1 type=dc
            goal Vout = DC1.V("out") analysis=DC1 ge 0 use=yield
            """;
        var r = CornerRun.Create(PreparedCircuit.FromText(cnl, null, null)).Run();

        Assert.Equal(StatisticalOutcome.Finished, r.Outcome);
        Assert.Equal(1200.0 / 2200, r.Corners[0].Goals.Single().WorstValue, 6);    // r1_typ, r2_hi (to the DC solve's tolerance)
        Assert.Equal(1200.0 / 2300, r.Corners[1].Goals.Single().WorstValue, 6);    // r1_hi, and still r2_hi
    }
}

/// <summary>R-ya6-3: a Monte Carlo at a corner is the run of the design with that corner's values typed.</summary>
public sealed class CornerMonteCarloTests
{
    [Fact]
    public void EachCornersYield_IsTheRunWithItsBindingsTyped()
    {
        const string spread = "\ntune R1.R dist=gauss sd=3%\ntune R2.R dist=gauss sd=3%\nstatistics trials=40\n";
        var r = CornerRun.Create(PreparedCircuit.FromText(CornerCircuits.Divider + spread + CornerCircuits.Corners, null, null),
                                 new CornerOptions { MonteCarlo = true }).Run();
        Assert.Equal(["nominal", "cold", "hot", "hiR2"], r.Yields.Select(y => y.Name));

        var typed = new[]
        {
            CornerCircuits.Divider + spread,
            CornerCircuits.Divider + spread + "temp = -40\n",
            CornerCircuits.Divider + spread + "temp = 125\n",
            CornerCircuits.Divider.Replace("out 0   R=1000 Ohm", "out 0   R=1100 Ohm") + spread + "temp = 85\n",
        };
        for (int i = 0; i < typed.Length; i++)
        {
            var alone = YieldCircuits.Create(typed[i]).Run();
            var at = r.Yields[i].Result;
            Assert.Equal(alone.Yield.Passes, at.Yield.Passes);
            Assert.Equal(alone.Yield.Counted, at.Yield.Counted);
            Assert.Equal(alone.Records.Select(t => t.Goals.Single().Margin), at.Records.Select(t => t.Goals.Single().Margin));
        }
        Assert.Equal("hot", r.WorstYield);
        Assert.Equal(["corner", "trial"], r.Data!["trials.pass"].Axes.Select(a => a.Name));
    }
}

/// <summary>R-ya6-4: a statistical corner replays its trial's z-vector against the current nominal.</summary>
public sealed class StatisticalCornerTests
{
    // R1's spread is a percent, R2's absolute.
    private static string Bench(double r1 = 1000, double r2 = 1000, string r1Name = "R1") => $"""
        Vdc:V1 in  0   Vdc=1
        R:{r1Name}   in  out R={r1} Ohm
        R:R2   out 0   R={r2} Ohm
        analysis DC1 type=dc
        goal Vout = DC1.V("out") analysis=DC1 in 0.49 0.51 use=yield
        tune {r1Name}.R dist=gauss sd=2%
        tune R2.R dist=gauss sd=20 Ohm
        statistics trials=20 seed=5
        """;

    private static double Ohms(string text) { Assert.True(TunableValue.TryParse(text, out _, out _, out double si)); return si; }

    [Fact]
    public void AReplay_ReproducesItsTrial_MovesWithAPercentSpread_AndNamesARenamedStream()
    {
        var mc = YieldCircuits.Create(Bench()).Run();
        var corner = Assert.Single(StatisticalCorner.FromRun(mc, "Vout", 1));
        var record = mc.Records[corner.Trial!.Value - 1];
        TuningSetup? setup = null;   // the circuit's own

        // The trial, exactly — from the recorded vector and drawn afresh alike.
        var replayed = StatisticalCorner.Replay(PreparedCircuit.FromText(Bench(), null, null), setup, corner, RecordedTrial.Of(record));
        Assert.Null(replayed.Refusal);
        Assert.Equal(record.Values, replayed.Values);
        Assert.Equal(record.Values, StatisticalCorner.Replay(PreparedCircuit.FromText(Bench(), null, null), setup, corner, (RecordedTrial?)null).Values);

        // Both nominals moved to 1.2 kΩ: the percent spread scales with it, the absolute one keeps its width.
        var moved = StatisticalCorner.Replay(PreparedCircuit.FromText(Bench(1200, 1200), null, null), setup, corner, RecordedTrial.Of(record));
        Assert.Equal((Ohms(record.Values["R1.R"]) - 1000) / 1000, (Ohms(moved.Values["R1.R"]) - 1200) / 1200, 12);
        Assert.Equal(Ohms(record.Values["R2.R"]) - 1000, Ohms(moved.Values["R2.R"]) - 1200, 9);

        // R1 renamed R9: its recorded stream is named, and the new entry stays at its nominal.
        var renamed = StatisticalCorner.Replay(PreparedCircuit.FromText(Bench(r1Name: "R9"), null, null), setup, corner, RecordedTrial.Of(record));
        var warning = Assert.Single(renamed.Notes);
        Assert.Equal("yield.corner.streams-gone", warning.Id);
        Assert.Contains("R1.R", warning.Render());
        Assert.False(renamed.Values.ContainsKey("R9.R"));
        Assert.Equal(record.Values["R2.R"], renamed.Values["R2.R"]);
    }
}

/// <summary>R-ya6-5: the cross product names its corners from their parts and refuses past the cap.</summary>
public sealed class CornerGeneratorTests
{
    [Fact]
    public void TwoOptionsThreeTempsTwoValues_AreTwelveNamedCorners_AndThreeHundredIsRefused()
    {
        var g = CornerGenerator.CrossProduct(
            [new GeneratorAxis("Kit|models/proc.lib", "proc", ["tt", "ss"])], ["-40", "25", "85"],
            [new GeneratorValues("Vdd", ["3.0 V", "3.6 V"])]);
        Assert.Null(g.Refusal);
        Assert.Equal(12, g.Corners.Count);
        Assert.Equal(12, g.Corners.Select(c => c.Name).Distinct().Count());
        var ss85 = g.Corners.Single(c => c.Name == "ss_85_Vdd3p0");
        Assert.Equal(("ss", "85", "3.0 V"), (ss85.AxisSelections!["Kit|models/proc.lib"], ss85.Temp, ss85.Values["Vdd"]));
        Assert.Contains(g.Corners, c => c.Name == "tt_m40_Vdd3p6");

        var big = CornerGenerator.CrossProduct([], ["-40", "25", "85"], [new GeneratorValues("Vdd", [.. Enumerable.Range(1, 100).Select(i => $"{i}")])]);
        Assert.Empty(big.Corners);
        Assert.Equal("yield.corner.generate-too-many", big.Refusal!.Id);
        Assert.Contains("300", big.Refusal.Render());
    }
}

/// <summary>R-ya6-6: the verb as a process.</summary>
public sealed class CornerCliTests
{
    [Fact]
    public void Corners_ExitsThreeNamingTheFailingCornerAndGoal_AndGenerateWritesNothing()
    {
        string dir = OptCli.Dir();
        string cnl = OptCli.Write(dir, "div.cnl", CornerCircuits.Divider + "\n" + CornerCircuits.Corners);

        var (exit, stdout, stderr) = OptCli.Run("yield", "corners", cnl);
        Assert.True(exit == 3, stderr);
        Assert.Matches(@"(?m)^  hot\s+125\s+\S+ ✗\s+FAILS$", stdout);
        Assert.Contains("Vout: hot", stdout);
        Assert.True(File.Exists(Path.Combine(dir, "div.corners.npy")));

        var before = Directory.GetFiles(dir).Select(f => (f, File.ReadAllBytes(f))).ToList();
        var (gen, lines, genErr) = OptCli.Run("yield", "corners", cnl, "--generate", "temp=-40,25;Vdd=3.0,3.6");
        Assert.True(gen == 0, genErr);
        Assert.Contains("corner tm40_Vdd3p0 temp=-40 Vdd=3.0", lines);
        Assert.Equal(4, lines.Split('\n').Count(l => l.StartsWith("corner ", StringComparison.Ordinal)));
        Assert.Equal(before.Select(b => b.f), Directory.GetFiles(dir));
        foreach (var (f, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(f));
    }
}
